#nullable enable
using System;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Arena.Tests.Editor
{
    public sealed class EditModeVerificationTests
    {
        private static readonly Type Evidence = Assembly.Load("Assembly-CSharp-Editor")
            .GetType("Arena.Editor.EditModeVerification", throwOnError: true)!;
        private static object Create(params string[] selectors) =>
            Activator.CreateInstance(Evidence, new object?[] { selectors, null })!;
        private static object? Invoke(object evidence, string method, params object[] args) =>
            Evidence.GetMethod(method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                .Invoke(evidence, args);
        private static string[] Errors(object evidence) => (string[])Invoke(evidence, "Errors")!;
        private static void Pass(object evidence, string name)
        {
            Invoke(evidence, "Select", name);
            Invoke(evidence, "Record", name, "Passed", "");
        }

        [Test]
        public void EmptyCompletedRunFails()
        {
            object evidence = Create("Example.Fixture");
            Invoke(evidence, "Complete");
            Assert.That(Errors(evidence), Has.Some.Contains("No tests passed"));
            Assert.That(Errors(evidence), Has.Some.Contains("required selector"));
        }

        [Test]
        public void OnePassingFixtureCannotHideAMissingRequestedFixture()
        {
            object evidence = Create("Example.First", "Example.Missing");
            Pass(evidence, "Example.First.Case");
            Invoke(evidence, "Complete");
            Assert.That(Errors(evidence), Has.Some.Contains("Example.Missing"));
        }

        [Test]
        public void IncompleteRunFailsEvenAfterPassingResults()
        {
            object evidence = Create("Example.Fixture");
            Pass(evidence, "Example.Fixture.Case");
            Assert.That(Errors(evidence), Has.Some.Contains("did not complete"));
        }

        [TestCase("Skipped")]
        [TestCase("Inconclusive")]
        [TestCase("Failed")]
        public void NonPassingResultFailsAlongsidePassingResults(string status)
        {
            object evidence = Create("Example.Fixture");
            Pass(evidence, "Example.Fixture.Passing");
            Invoke(evidence, "Select", "Example.Fixture.Other");
            Invoke(evidence, "Record", "Example.Fixture.Other", status, "reason");
            Invoke(evidence, "Complete");
            Assert.That(Errors(evidence), Has.Some.Contains(status));
        }

        [Test]
        public void SelectedCaseMustReportAResult()
        {
            object evidence = Create("Example.Fixture");
            Pass(evidence, "Example.Fixture.Passing");
            Invoke(evidence, "Select", "Example.Fixture.Missing");
            Invoke(evidence, "Complete");
            Assert.That(Errors(evidence), Has.Some.Contains("Selected test did not report"));
        }

        [Test]
        public void ParameterizedCasesSatisfyAnExactMethodSelector()
        {
            object evidence = Create("Example.Fixture.Case");
            Pass(evidence, "Example.Fixture.Case(1)");
            Pass(evidence, "Example.Fixture.Case(2)");
            Invoke(evidence, "Complete");
            Assert.That(Errors(evidence), Is.Empty);
        }

        [Test]
        public void SimilarlyNamedFixtureCannotSatisfySelector()
        {
            object evidence = Create("Example.Fixture");
            Pass(evidence, "Example.FixtureExtra.Case");
            Invoke(evidence, "Complete");
            Assert.That(Errors(evidence), Has.Some.Contains("required selector"));
        }

        [Test]
        public void NamespaceFilterDoesNotSelectAnEntireSimilarlyNamedAssembly()
        {
            string pattern = (string)Evidence.GetMethod("GroupPattern", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, new object[] { "Arena.EditModeTests" })!;
            Assert.That(Regex.IsMatch("Arena.EditModeTests.dll", pattern), Is.False);
            Assert.That(Regex.IsMatch("Arena.Tests.Editor.SomeOtherFixture.Case", pattern), Is.False);
            Assert.That(Regex.IsMatch("Arena.EditModeTests.ConnectionFeedbackHudTests.Case", pattern), Is.True);
        }

        [Test]
        public void DuplicateResultsFail()
        {
            object evidence = Create("Example.Fixture");
            Pass(evidence, "Example.Fixture.Case");
            Pass(evidence, "Example.Fixture.Case");
            Invoke(evidence, "Complete");
            Assert.That(Errors(evidence), Has.Some.Contains("Duplicate"));
        }
    }
}
