#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace Arena.Editor
{
    /// <summary>Requires named coverage and successful leaf results, including when a filter matches nothing.</summary>
    internal sealed class EditModeVerification : ICallbacks
    {
        private readonly string[] _selectors;
        private readonly string? _xmlPath;
        private readonly HashSet<string> _selected = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _results = new(StringComparer.Ordinal);
        private readonly List<string> _failures = new();
        public bool Completed { get; private set; }
        public int Passed => _results.Count(pair => pair.Value == "Passed");
        public int Failed => _results.Count(pair => pair.Value != "Passed");
        public string[] ExecutedNames => _results.Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray();

        public EditModeVerification(string[] selectors, string? xmlPath = null)
        {
            if (selectors.Length == 0 || selectors.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException("At least one explicit test selector is required.", nameof(selectors));
            _selectors = selectors;
            _xmlPath = xmlPath;
        }

        internal static bool Matches(string selector, string name) => name == selector
            || name.StartsWith(selector + ".", StringComparison.Ordinal)
            || name.StartsWith(selector + "(", StringComparison.Ordinal);

        // Unity matches assembly names too. Arena.EditModeTests is both a namespace and an
        // assembly: matching Arena.EditModeTests.dll would silently select every test in it.
        internal static string GroupPattern(string selector) =>
            @"^(?!.*\.dll$)" + Regex.Escape(selector) + @"(?:$|\.|\()";

        internal void Select(string name)
        {
            if (_selectors.Any(selector => Matches(selector, name))) _selected.Add(name);
        }

        internal void Record(string name, string status, string detail = "")
        {
            if (!_selectors.Any(selector => Matches(selector, name)))
                _failures.Add("Unexpected test result: " + name);
            if (_results.ContainsKey(name)) _failures.Add("Duplicate test result: " + name);
            _results[name] = status;
            // Full NUnit details remain in XML; source-text assertions can otherwise repeat megabytes in the log.
            if (status != "Passed") _failures.Add(name + ": " + status + " "
                + (detail.Length <= 1500 ? detail : detail.Substring(0, 1500) + " [full detail in tests.xml]"));
        }

        internal void Complete() => Completed = true;

        public string[] Errors()
        {
            var errors = new List<string>(_failures);
            if (!Completed) errors.Add("Test run did not complete.");
            if (_selected.Count == 0) errors.Add("No selected tests were discovered.");
            if (Passed == 0) errors.Add("No tests passed.");
            foreach (string selector in _selectors)
                if (!_results.Keys.Any(name => Matches(selector, name)))
                    errors.Add("No executed tests matched required selector: " + selector);
            foreach (string name in _selected.Where(name => !_results.ContainsKey(name)))
                errors.Add("Selected test did not report a result: " + name);
            return errors.ToArray();
        }

        internal static EditModeVerification Run(string[] selectors, string? xmlPath = null)
        {
            var evidence = new EditModeVerification(selectors, xmlPath);
            var runner = ScriptableObject.CreateInstance<TestRunnerApi>();
            runner.RegisterCallbacks(evidence);
            try
            {
                runner.Execute(new ExecutionSettings(new Filter
                {
                    testMode = TestMode.EditMode,
                    // Namespaces and fixtures are prefixes, not exact NUnit test names.
                    // Both first-party namespaces must be eligible regardless of their assembly.
                    groupNames = selectors.Select(GroupPattern).ToArray(),
                }) { runSynchronously = true });
            }
            finally
            {
                runner.UnregisterCallbacks(evidence);
                UnityEngine.Object.DestroyImmediate(runner);
            }
            return evidence;
        }

        public void RunStarted(ITestAdaptor tests) => CaptureSelected(tests);
        private void CaptureSelected(ITestAdaptor test)
        {
            if (!test.IsSuite) Select(test.FullName);
            if (test.HasChildren)
                foreach (ITestAdaptor child in test.Children) CaptureSelected(child);
        }
        public void TestStarted(ITestAdaptor test) { }
        public void TestFinished(ITestResultAdaptor result) { }
        public void RunFinished(ITestResultAdaptor result)
        {
            CaptureResults(result);
            if (result.TestStatus == TestStatus.Failed && _failures.Count == 0)
                _failures.Add("Test suite failed: " + result.Message);
            Complete();
            if (_xmlPath != null) TestRunnerApi.SaveResultToFile(result, _xmlPath);
        }
        private void CaptureResults(ITestResultAdaptor result)
        {
            if (!result.Test.IsSuite) Record(result.FullName, result.TestStatus.ToString(), result.Message);
            if (result.HasChildren)
                foreach (ITestResultAdaptor child in result.Children) CaptureResults(child);
        }
    }
}
