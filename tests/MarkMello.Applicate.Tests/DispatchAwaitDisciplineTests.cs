using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MarkMello.Applicate.Tests;

// Guard against reintroducing the defect fixed by
// work-items/bugs/2026-07-26-headless-dispatch-swallows-assertions.md:
// a non-async test method that calls session.Dispatch(() => { ... },
// CancellationToken.None) and discards the returned Task sends every
// exception thrown inside the lambda -- including an xUnit assertion
// failure -- to a faulted Task nobody ever observes. The test then reports
// Passed regardless of what the lambda actually asserted.
//
// The safe idioms already in this repo are (a) `await session.Dispatch(...)`
// and (b) `session.Dispatch(...).GetAwaiter().GetResult();` (the latter
// often spanning multiple lines, e.g. ApplicateSharedWebViewHostRealHostTests
// .RunOnHost and IpcContractTests.RunOnView). A NAIVE same-line grep for
// `Dispatch(` without `await` over-counts, because the `.GetAwaiter()` can
// sit on a later line -- this scan is STATEMENT-aware: it walks from each
// `.Dispatch(` call site to the statement-terminating `;` at bracket depth
// zero (so a `;` inside the dispatched lambda body does not end the scan
// early), then checks the whole statement for `await` immediately preceding
// the call or `GetAwaiter` anywhere within it.
public static class DispatchAwaitScanner
{
    // Matches `<identifier>.Dispatch(`, optionally preceded by `await `.
    // HeadlessUnitTestSession is dispatched through variables named
    // differently across this project (`session`, `headless`), so the
    // receiver identifier is intentionally NOT hardcoded to "session".
    private static readonly Regex DispatchCallSite = new(
        @"(?<awaited>\bawait\s+)?\b[A-Za-z_][A-Za-z0-9_]*\.Dispatch\(",
        RegexOptions.Compiled);

    public static IReadOnlyList<string> FindUnobservedDispatchStatements(string source, string fileLabel)
    {
        var violations = new List<string>();

        foreach (Match match in DispatchCallSite.Matches(source))
        {
            if (match.Groups["awaited"].Success)
            {
                continue;
            }

            var statementEnd = FindStatementEnd(source, match.Index);
            var statement = source[match.Index..statementEnd];
            if (statement.Contains("GetAwaiter", StringComparison.Ordinal))
            {
                continue;
            }

            var line = source[..match.Index].Count(c => c == '\n') + 1;
            violations.Add(
                $"{fileLabel}:{line}: '{match.Value.TrimEnd('(')}(' is neither awaited nor terminated with " +
                ".GetAwaiter().GetResult() -- any exception thrown inside the dispatched lambda (including an " +
                "xUnit assertion failure) will land on an unobserved faulted Task and the test will report Passed.");
        }

        return violations;
    }

    // Walks from the start of the `.Dispatch(` call to the statement-
    // terminating `;` at bracket depth zero, so a `;` inside the dispatched
    // lambda body (or inside any nested call) never ends the scan early.
    private static int FindStatementEnd(string source, int startIndex)
    {
        var depth = 0;
        for (var i = startIndex; i < source.Length; i++)
        {
            switch (source[i])
            {
                case '(' or '{' or '[':
                    depth++;
                    break;
                case ')' or '}' or ']':
                    depth--;
                    break;
                case ';' when depth == 0:
                    return i + 1;
            }
        }

        return source.Length;
    }
}

public sealed class DispatchAwaitDisciplineTests
{
    // ---- the real guard: scans every .cs file this test project owns -----

    [Fact]
    public void EveryDispatchCallSiteInTheTestProjectIsAwaitedOrSynchronouslyUnwrapped()
    {
        var testProjectDirectory = FindTestProjectDirectory();
        var violations = new List<string>();

        foreach (var path in Directory.EnumerateFiles(testProjectDirectory, "*.cs", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(testProjectDirectory, path);
            var segments = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Contains("bin", StringComparer.Ordinal) || segments.Contains("obj", StringComparer.Ordinal))
            {
                // Build output / generated sources (GlobalUsings.g.cs, etc.) --
                // not hand-authored test code this guard is meant to police.
                continue;
            }

            if (string.Equals(Path.GetFileName(path), "DispatchAwaitDisciplineTests.cs", StringComparison.Ordinal))
            {
                // This file's own fixtures below are deliberately-bad string
                // LITERALS the scanner-logic tests assert against directly --
                // not live Dispatch call sites. The scanner-logic tests are
                // what police this file's real (non-fixture) code.
                continue;
            }

            var source = File.ReadAllText(path);
            violations.AddRange(
                DispatchAwaitScanner.FindUnobservedDispatchStatements(source, relativePath));
        }

        Assert.Empty(violations);
    }

    // ---- scanner-logic fixtures: proves the detector actually discriminates
    // bad from good BEFORE trusting it to gate the real project scan above.
    // A detector that always reports "no violations" would make the guard
    // above vacuously green forever -- these fixtures are the mutation proof
    // that it does not.

    [Fact]
    public void ScannerFlagsTheExactFireAndForgetPatternThatSwallowedAssertions()
    {
        // The exact shape this whole fix addresses: a non-async test body
        // that calls session.Dispatch(...) and discards the Task. An
        // Assert.Fail inside this lambda was empirically proven (the bug
        // report) to still report the test as Passed.
        const string badFixture = """
            public void SomeTest()
            {
                var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
                session.Dispatch(() =>
                {
                    Assert.Fail("this assertion would be silently swallowed");
                }, CancellationToken.None);
            }
            """;

        var violations = DispatchAwaitScanner.FindUnobservedDispatchStatements(badFixture, "fixture.cs");

        Assert.Single(violations);
        Assert.Contains("session.Dispatch(", violations[0]);
    }

    [Fact]
    public void ScannerAllowsTheAwaitedIdiom()
    {
        const string goodFixture = """
            public async Task SomeTest()
            {
                var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
                await session.Dispatch(() =>
                {
                    Assert.Fail("this assertion now genuinely fails the test");
                }, CancellationToken.None);
            }
            """;

        Assert.Empty(DispatchAwaitScanner.FindUnobservedDispatchStatements(goodFixture, "fixture.cs"));
    }

    [Fact]
    public void ScannerAllowsTheMultilineGetAwaiterGetResultIdiom()
    {
        // Mirrors ApplicateSharedWebViewHostRealHostTests.RunOnHost and
        // IpcContractTests.RunOnView: the safe idiom for a helper whose own
        // signature cannot be made async.
        const string goodFixture = """
            private static void RunOnHost(Action<Host> body)
            {
                var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
                session.Dispatch(() => body(NewHost()), CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            }
            """;

        Assert.Empty(DispatchAwaitScanner.FindUnobservedDispatchStatements(goodFixture, "fixture.cs"));
    }

    [Fact]
    public void ScannerDoesNotFlagADifferentlyNamedReceiverVariable()
    {
        // BulkCloseSessionLossReproTests.cs / EditWorkspace*Tests.cs dispatch
        // through a variable named `headless`, not `session` -- the scanner
        // must not be hardcoded to one receiver name.
        const string badFixture = """
            public void SomeTest()
            {
                var headless = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
                headless.Dispatch(() =>
                {
                    Assert.Fail("swallowed regardless of the receiver's name");
                }, CancellationToken.None);
            }
            """;

        var violations = DispatchAwaitScanner.FindUnobservedDispatchStatements(badFixture, "fixture.cs");

        Assert.Single(violations);
        Assert.Contains("headless.Dispatch(", violations[0]);
    }

    private static string FindTestProjectDirectory()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "MarkMello.Applicate.Tests.csproj");
                if (File.Exists(candidate))
                {
                    return directory.FullName;
                }
            }
        }

        throw new DirectoryNotFoundException("Could not locate the MarkMello.Applicate.Tests project directory.");
    }
}
